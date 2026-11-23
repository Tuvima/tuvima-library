package com.tuvima.library.core

// Android Auto (spec native-apps WP11, issue 787): what the car's list holds, in which order, and what the Now playing card's extra buttons
// are. The car draws everything itself from lists and one card, so nothing here draws anything: these are the rules the media service turns
// into its browse tree, its search and its buttons. Every name and order is the contract's "Cars" section
// (clients/contracts/v1/player-controls.md); a test compares it with the file. Nothing is added on the Engine's side: every list is a read the
// phone already makes, and the Engine alone decides what the signed-in profile may see (a Kids profile gets the Kids list), so no rule here
// filters by profile. Driving safety: only songs and audiobooks ever appear (no video, no pictures to browse), and each list is short.

/**
 * One entry of the car list. The declaration order IS the contract's order. [contractName] is the contract table's word for the row;
 * [title] is what the car shows ("The Listen shelves" is the Dashboard's Listen page, which the car shows as "Listen").
 */
enum class CarEntry(val contractName: String, val title: String, val nodeId: String) {
    Continue("Continue", "Continue", "car:continue"),
    ListenShelves("The Listen shelves", "Listen", "car:listen"),
    Audiobooks("Audiobooks", "Audiobooks", "car:audiobooks"),
    Playlists("Playlists", "Playlists", "car:playlists"),
    RecentlyPlayed("Recently played", "Recently played", "car:recent"),
}

/** A place in the car's browse tree, read back from the id the media service gave it. */
sealed interface CarNode {
    data object Root : CarNode
    data class Entry(val entry: CarEntry) : CarNode

    /** One shelf of the Listen page (the Engine's own shelf key). */
    data class ListenShelf(val key: String) : CarNode

    /** An album, opened by the Details page's own (type, id) pair; its songs are the list. */
    data class Album(val entityType: String, val id: String) : CarNode

    /** A search result that plays: [audiobook] says which kind, so a voice request never starts the wrong thing. */
    data class Hit(val audiobook: Boolean, val entityType: String, val id: String) : CarNode

    /** A song or audiobook file in a list. */
    data class Play(val assetId: String) : CarNode
}

object CarList {
    const val ROOT_ID = "tuvima-root"

    /** Android Auto shows the root's entries as tabs and has room for four. The shipped list is four (see [PLAYLISTS_AVAILABLE]). */
    const val ROOT_TAB_LIMIT = 4

    /** A list in the car stays short: the first 50 are read, and the car pages through them by the size it asks for. */
    const val NODE_LIMIT = 50

    /**
     * Playlists are in the contract's list, but the phone's connection has no route that reads them (the Engine's collection routes leave
     * playlists out; see engineering/validation/native-listen-lanes-2026-11.md), so the row is left out until the Engine has one. Turning
     * this on needs a decision about the four-tab root as well, because five entries do not fit it.
     */
    const val PLAYLISTS_AVAILABLE = false

    /** The Engine's own shelf keys that the Listen entry does not repeat: Recently played is an entry of its own, and artists have no car list. */
    private val LISTEN_SHELVES_LEFT_OUT = setOf("recently-played", "artists")

    const val RECENTLY_PLAYED_SHELF = "recently-played"

    private const val SEP = ":"
    private const val PLAY = "car:play"
    private const val SHELF = "car:listen"
    private const val ALBUM = "car:album"
    private const val HIT = "car:hit"

    /** The root's entries in the contract's order, without the ones the phone cannot read yet. */
    fun rootEntries(playlistsAvailable: Boolean = PLAYLISTS_AVAILABLE): List<CarEntry> =
        CarEntry.entries.filter { playlistsAvailable || it != CarEntry.Playlists }

    fun shelfId(key: String): String = "$SHELF$SEP$key"

    fun albumId(entityType: String, id: String): String = "$ALBUM$SEP$entityType$SEP$id"

    fun hitId(audiobook: Boolean, entityType: String, id: String): String = "$HIT$SEP${if (audiobook) "a" else "m"}$SEP$entityType$SEP$id"

    fun playId(assetId: String): String = "$PLAY$SEP$assetId"

    /** What an id from the car points at; null for an id this app never gave out. */
    fun parse(id: String?): CarNode? {
        if (id.isNullOrBlank()) return null
        if (id == ROOT_ID) return CarNode.Root
        CarEntry.entries.firstOrNull { it.nodeId == id }?.let { return CarNode.Entry(it) }
        val parts = id.split(SEP, limit = 4)
        return when (parts.firstOrNull()) {
            "car" -> when (parts.getOrNull(1)) {
                "listen" -> parts.getOrNull(2)?.takeIf(String::isNotBlank)?.let { CarNode.ListenShelf(id.removePrefix("$SHELF$SEP")) }
                "play" -> id.removePrefix("$PLAY$SEP").takeIf { it.isNotBlank() && it != id }?.let { CarNode.Play(it) }
                "album" -> {
                    val type = parts.getOrNull(2)?.takeIf(String::isNotBlank)
                    val key = parts.getOrNull(3)?.takeIf(String::isNotBlank)
                    if (type != null && key != null) CarNode.Album(type, key) else null
                }
                "hit" -> {
                    val rest = id.removePrefix("$HIT$SEP").split(SEP, limit = 3)
                    val kind = rest.getOrNull(0)
                    val type = rest.getOrNull(1)?.takeIf(String::isNotBlank)
                    val key = rest.getOrNull(2)?.takeIf(String::isNotBlank)
                    if ((kind == "a" || kind == "m") && type != null && key != null) CarNode.Hit(kind == "a", type, key) else null
                }
                else -> null
            }
            else -> null
        }
    }

    /** The Audiobooks entry's read: the Listen lane's audiobooks, newest first. */
    fun audiobooksQuery(): BrowseQuery = BrowseQuery(
        lane = ListenLogic.LANE,
        mediaType = "Audiobook",
        sort = "newest",
        offset = 0,
        limit = NODE_LIMIT,
    )

    /** The shelves the Listen entry shows, each as a folder of its own, with the Engine's own titles. Shelves with nothing playable are left out. */
    fun listenShelves(page: DisplayPage): List<DisplayShelf> =
        page.shelves
            .filter { it.key !in LISTEN_SHELVES_LEFT_OUT && it.title.isNotBlank() && CarCards.items(it.items).isNotEmpty() }
            .take(NODE_LIMIT)

    /** The part of a list the car asked for. A page or size that makes no sense gives the first [NODE_LIMIT]. */
    fun <T> slice(items: List<T>, page: Int, pageSize: Int): List<T> {
        val size = if (pageSize <= 0) NODE_LIMIT else minOf(pageSize, NODE_LIMIT)
        val start = (maxOf(page, 0).toLong() * size).coerceAtMost(items.size.toLong()).toInt()
        return items.subList(start, minOf(start + size, items.size))
    }
}

enum class CarCardKind { Song, Album, Audiobook }

/**
 * One row of a car list. A song or audiobook has [assetId] (the file that plays); an album has [detailType] and [detailId] (the Details page
 * that lists its songs).
 */
data class CarCard(
    val kind: CarCardKind,
    val title: String,
    val line: String?,
    val assetId: String? = null,
    val workId: String? = null,
    val artist: String? = null,
    val album: String? = null,
    val artworkUrl: String? = null,
    val detailType: String? = null,
    val detailId: String? = null,
) {
    /** The song as a queue entry; null for anything else. */
    fun toTrack(): QueueTrack? =
        if (kind == CarCardKind.Song && !assetId.isNullOrBlank()) {
            QueueTrack(assetId = assetId, title = title, artist = artist, album = album, artworkUrl = artworkUrl, workId = workId)
        } else {
            null
        }
}

object CarCards {
    /**
     * What a card is in the car, or null when it has no place there. Only audio is drawn: video, books, comics, pictures and anything with no
     * file to play (and no album page to open) are left out, so nothing in the car can start a video.
     */
    fun of(card: DisplayCard): CarCard? {
        val title = card.title.trim().ifEmpty { return null }
        val type = card.mediaType.lowercase()
        val art = card.squareArtUrl ?: card.posterUrl ?: card.artworkUrl
        return when {
            "audiobook" in type -> {
                val asset = card.playableAssetId()?.takeIf(String::isNotBlank) ?: return null
                CarCard(CarCardKind.Audiobook, title, card.subtitle?.trim()?.takeIf(String::isNotEmpty), assetId = asset, workId = card.workId, artworkUrl = art)
            }
            type == ListenLogic.MEDIA_TYPE.lowercase() -> when (card.groupingType) {
                "album" -> {
                    val target = card.actions.firstNotNullOfOrNull { detailTargetFromRoute(it.webUrl) } ?: return null
                    CarCard(
                        CarCardKind.Album, title, card.subtitle?.trim()?.takeIf(String::isNotEmpty),
                        artist = card.subtitle, artworkUrl = art, detailType = target.first, detailId = target.second,
                    )
                }
                "artist" -> null
                else -> {
                    val track = PlayQueue.trackOf(card) ?: return null
                    CarCard(
                        CarCardKind.Song, title, PlayQueue.secondLine(track),
                        assetId = track.assetId, workId = track.workId, artist = track.artist, album = track.album, artworkUrl = track.artworkUrl,
                    )
                }
            }
            else -> null
        }
    }

    /** The rows of a list: audio only, each file or album once, at most [CarList.NODE_LIMIT]. */
    fun items(cards: List<DisplayCard>): List<CarCard> {
        val seen = HashSet<String>()
        return cards.mapNotNull(::of)
            .filter { seen.add(keyOf(it)) }
            .take(CarList.NODE_LIMIT)
    }

    /** The songs of an album's Details page as rows, in the page's order. */
    fun songsOf(tracks: List<QueueTrack>): List<CarCard> =
        tracks.filter { it.assetId.isNotBlank() }.take(CarList.NODE_LIMIT).map { track ->
            CarCard(
                CarCardKind.Song, track.title, PlayQueue.secondLine(track),
                assetId = track.assetId, workId = track.workId, artist = track.artist, album = track.album, artworkUrl = track.artworkUrl,
            )
        }

    private fun keyOf(card: CarCard): String = when (card.kind) {
        CarCardKind.Album -> "album:${card.detailType}:${card.detailId}"
        else -> "file:${card.assetId}"
    }
}

/** What a press or a voice request starts. */
sealed interface CarPlan {
    /** These songs, from the one pointed at. */
    data class Songs(val request: PlayQueueRequest) : CarPlan

    /** One audiobook file. */
    data class Book(val assetId: String, val title: String, val author: String?, val workId: String?, val artworkUrl: String?) : CarPlan
}

object CarPlayPlan {
    /**
     * A press on a file in a list. A song plays the songs of that list from it (so the car goes on to the next song by itself); an audiobook
     * plays alone. Null when the list does not hold the file.
     */
    fun forPress(list: List<CarCard>, assetId: String, source: String?): CarPlan? {
        val pressed = list.firstOrNull { it.assetId == assetId } ?: return null
        return when (pressed.kind) {
            CarCardKind.Audiobook -> CarPlan.Book(assetId, pressed.title, pressed.line, pressed.workId, pressed.artworkUrl)
            CarCardKind.Song -> {
                val tracks = list.mapNotNull { it.toTrack() }
                val start = tracks.indexOfFirst { it.assetId == assetId }.takeIf { it >= 0 } ?: return null
                CarPlan.Songs(PlayQueue.normalise(PlayQueueRequest(tracks, start, shuffle = false, sourceName = clean(source))))
            }
            CarCardKind.Album -> null
        }
    }

    /** A press (or request) on an album's page: all its songs from the first. */
    fun forAlbum(detail: DetailModel): CarPlan? {
        val tracks = PlayQueue.tracksOf(detail.children, detail.title, detail.subtitle, detail.artworkUrl).filter { it.assetId.isNotBlank() }
        if (tracks.isEmpty()) return null
        return CarPlan.Songs(PlayQueue.normalise(PlayQueueRequest(tracks, 0, shuffle = false, sourceName = clean(detail.title))))
    }

    /** What a search result's own Details page starts: an album's songs, a song's file, or an audiobook's file. */
    fun forHit(hit: CarNode.Hit, detail: DetailModel): CarPlan? {
        return when (val target = DetailPlay.resolve(null, detail.children, detail.title, detail.subtitle, detail.artworkUrl)) {
            DetailPlayTarget.Nothing -> null
            is DetailPlayTarget.Songs ->
                CarPlan.Songs(PlayQueue.normalise(PlayQueueRequest(target.tracks, 0, shuffle = false, sourceName = target.sourceName)))
            is DetailPlayTarget.OneFile ->
                if (hit.audiobook) {
                    CarPlan.Book(target.assetId, detail.title, detail.subtitle, hit.id, detail.artworkUrl)
                } else {
                    val track = QueueTrack(target.assetId, detail.title, detail.subtitle, null, detail.artworkUrl, workId = hit.id)
                    CarPlan.Songs(PlayQueueRequest(listOf(track), 0, shuffle = false, sourceName = null))
                }
        }
    }

    /** A song named in a voice request when its page lists no file: the song list narrowed by the spoken title, the exact title first. */
    fun forSongTitle(title: String, cards: List<DisplayCard>): CarPlan? {
        val songs = CarCards.items(cards).filter { it.kind == CarCardKind.Song }
        val pick = songs.firstOrNull { it.title.equals(title.trim(), ignoreCase = true) } ?: songs.firstOrNull() ?: return null
        return forPress(songs, pick.assetId.orEmpty(), null)
    }

    private fun clean(source: String?): String? = source?.trim()?.takeIf(String::isNotEmpty)
}

/** What a voice request or a typed search asks for. */
sealed interface CarQuery {
    /** "Play music": carry on with what was playing. */
    data object Resume : CarQuery
    data class Text(val text: String) : CarQuery
    data object TooShort : CarQuery
}

object CarSearch {
    /** The most results one search shows in the car. */
    const val MAX_RESULTS = 10

    fun classify(spoken: String?): CarQuery {
        val text = spoken?.trim().orEmpty()
        if (text.isEmpty()) return CarQuery.Resume
        return SearchLogic.queryToSend(text)?.let { CarQuery.Text(it) } ?: CarQuery.TooShort
    }

    /** True for a result the car can play: a song, an album or an audiobook. Anything else (a film, a show, a book, a photo) is left out. */
    fun isAudio(hit: SearchHit): Boolean = isAudiobook(hit) || hit.mediaType.equals(ListenLogic.MEDIA_TYPE, ignoreCase = true)

    fun isAudiobook(hit: SearchHit): Boolean = "audiobook" in hit.mediaType.lowercase()

    /** The playable results in the Engine's own order: the top result, then each kind's list; a result is listed once. */
    fun audioHits(results: SearchResults, limit: Int = MAX_RESULTS): List<SearchHit> {
        val seen = HashSet<String>()
        val all = listOfNotNull(results.topResult) + results.sections.flatMap { it.hits }
        return all.filter { isAudio(it) && seen.add("${it.entityType.lowercase()}:${it.id.lowercase()}") }.take(limit.coerceAtLeast(0))
    }

    fun nodeOf(hit: SearchHit): CarNode.Hit = CarNode.Hit(isAudiobook(hit), hit.entityType, hit.id)
}

/** The listings the service has read, kept for the profile that read them: another profile never sees them (a Kids profile reads its own). */
class CarListings<T> {
    private var profileId: String? = null
    private val lists = LinkedHashMap<String, T>()

    /** Tells the cache who is signed in; a different profile starts it empty. True when that happened. */
    fun useProfile(id: String?): Boolean {
        if (id == profileId) return false
        lists.clear()
        profileId = id
        return true
    }

    operator fun get(key: String): T? = lists[key]

    operator fun set(key: String, value: T) {
        lists[key] = value
    }

    fun clear() = lists.clear()
}

/** The Now playing card's extra buttons and the contract's transport for the car. The card's lines come from [LockScreen] (the lock screen's own fields). */
object CarNowPlaying {
    /** The extras the card can carry; [command] is the session command each one sends. */
    enum class Extra(val command: String) {
        Shuffle("com.tuvima.library.CAR_SHUFFLE"),
        Repeat("com.tuvima.library.CAR_REPEAT"),
        Speed("com.tuvima.library.CAR_SPEED"),
    }

    data class ExtraButton(val extra: Extra, val label: String, val selected: Boolean)

    /**
     * The buttons beside the transport: music has Shuffle and Repeat; an audiobook has Speed. The car's card holds only a few extra buttons, so
     * Speed is one button that shows the speed and steps through the contract's presets ([nextSpeed]).
     */
    fun extraButtons(kind: TvMediaKind, shuffle: Boolean, repeat: TvRepeatMode, speed: Double): List<ExtraButton> = when (kind) {
        TvMediaKind.Music -> listOf(
            ExtraButton(Extra.Shuffle, "Shuffle", shuffle),
            ExtraButton(Extra.Repeat, RepeatCycle.label(repeat), repeat != TvRepeatMode.Off),
        )
        TvMediaKind.Audiobook -> listOf(ExtraButton(Extra.Speed, TvPlayerLogic.speedLabel(speed), selected = false))
        else -> emptyList()
    }

    /** The preset after the speed now set (the next one up, then round to the first). */
    fun nextSpeed(current: Double): Double {
        val now = TvPlayerLogic.clampSpeed(current)
        return TvPlayerLogic.SPEED_PRESETS.firstOrNull { it > now + 0.0001 } ?: TvPlayerLogic.SPEED_PRESETS.first()
    }

    /**
     * The transport the contract names for the car, as the system's own action names: music has Previous, Play and Next; an audiobook has back
     * and forward by the profile's skip seconds and, with chapters, Previous chapter and Next chapter (Play is the template's own button).
     */
    fun transport(kind: TvMediaKind, hasChapters: Boolean): List<String> = when (kind) {
        TvMediaKind.Music -> listOf(LockAction.PREVIOUS_TRACK, LockAction.PLAY, LockAction.NEXT_TRACK)
        TvMediaKind.Audiobook -> buildList {
            add(LockAction.SEEK_BACKWARD)
            add(LockAction.SEEK_FORWARD)
            if (hasChapters) {
                add(LockAction.PREVIOUS_TRACK)
                add(LockAction.NEXT_TRACK)
            }
        }
        else -> emptyList()
    }
}

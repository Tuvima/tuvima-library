package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject

// My List on the phone: the saved titles of the active profile (`/api/v1/profile-state/saved`). Pure rules and wire reading only;
// nothing here draws anything or touches the network.

/**
 * What My List stores a title as. [wire] is the number the Engine sends and reads for its `ProfileEntityKind`, [route] the name its
 * `/saved/{entityKind}/{id}` routes accept (any letter case).
 */
enum class SavedKind(val wire: Int, val route: String) {
    Movie(0, "movie"),
    TvShow(1, "tvshow"),
    Book(2, "book"),
    Comic(3, "comic"),
    Audiobook(4, "audiobook"),
    Album(5, "album"),
    Song(6, "song"),
    Collection(7, "collection"),
    Playlist(8, "playlist"),
    ;

    companion object {
        /** Reads the Engine's `entityKind`, which arrives as a number (or, being lenient, as the kind's name). Null when unknown. */
        fun fromWire(value: Any?): SavedKind? = when (value) {
            is Number -> entries.firstOrNull { it.wire == value.toInt() }
            is String -> value.trim().let { text ->
                text.toIntOrNull()?.let { number -> entries.firstOrNull { it.wire == number } }
                    ?: entries.firstOrNull { it.route.equals(text, ignoreCase = true) || it.name.equals(text, ignoreCase = true) }
            }
            else -> null
        }
    }
}

/** One saved title: its kind and the id My List knows it by (the show for an episode). The id is kept in lower case. */
data class SavedKey(val kind: SavedKind, val id: String) {
    companion object {
        fun of(kind: SavedKind, id: String): SavedKey = SavedKey(kind, id.trim().lowercase())
    }
}

/** A saved title as the Engine returns it. [savedAt] is the Engine's own text, kept so Undo can put the title back with its original date. */
data class SavedItem(val key: SavedKey, val savedAt: String?, val position: Int?)

/**
 * The title a details route or card stands for, as My List stores it. Null when My List holds no such kind from this phone:
 * a Series or curated collection group, a playlist, a person or a photo. An episode is saved as its show ([showId]; null without one).
 */
fun savedKeyForEntity(entityType: String, id: String, showId: String? = null): SavedKey? {
    val type = entityType.trim().lowercase()
    if (id.isBlank()) return null
    return when {
        "episode" in type -> showId?.takeIf(String::isNotBlank)?.let { SavedKey.of(SavedKind.TvShow, it) }
        "audiobook" in type -> SavedKey.of(SavedKind.Audiobook, id)
        "comic" in type -> SavedKey.of(SavedKind.Comic, id)
        "book" in type -> SavedKey.of(SavedKind.Book, id)
        "movie" in type -> SavedKey.of(SavedKind.Movie, id)
        "tv" in type || "show" in type -> SavedKey.of(SavedKind.TvShow, id)
        "album" in type || "music" in type -> SavedKey.of(SavedKind.Album, id)
        else -> null
    }
}

/** The Engine's list of saved titles. A row with an unknown kind or no id is skipped. */
fun parseSavedItems(array: JSONArray): List<SavedItem> = buildList {
    for (index in 0 until array.length()) {
        val row = array.optJSONObject(index) ?: continue
        val kind = SavedKind.fromWire(row.opt("entityKind")) ?: continue
        val id = row.optionalString("entityId") ?: continue
        add(
            SavedItem(
                key = SavedKey.of(kind, id),
                savedAt = row.optionalString("savedAt"),
                position = row.optionalInt("position"),
            ),
        )
    }
}

/** Body of `POST /saved/remove`: the titles to take off My List. */
fun removeSavedBody(keys: List<SavedKey>): JSONObject = JSONObject().put(
    "items",
    JSONArray().apply {
        keys.forEach { key -> put(JSONObject().put("entityKind", key.kind.wire).put("entityId", key.id)) }
    },
)

/** Body of `POST /saved/restore`: the titles a remove returned, to save again with their original dates. */
fun restoreSavedBody(items: List<SavedItem>): JSONObject = JSONObject().put(
    "items",
    JSONArray().apply {
        items.forEach { item ->
            put(
                JSONObject()
                    .put("entityKind", item.key.kind.wire)
                    .put("entityId", item.key.id)
                    .put("savedAt", item.savedAt ?: JSONObject.NULL)
                    .put("position", item.position ?: JSONObject.NULL),
            )
        }
    },
)

/**
 * The saved titles of this phone's profile, as the screens need them: "is this saved" for the plus and tick. Immutable; each change
 * returns the next state, so a screen can show a press at once and put the old state back when the Engine refuses.
 */
class SavedSet private constructor(private val keys: Set<SavedKey>) {
    val size: Int get() = keys.size

    operator fun contains(key: SavedKey): Boolean = key in keys

    fun with(key: SavedKey): SavedSet = if (key in keys) this else SavedSet(keys + key)

    fun without(key: SavedKey): SavedSet = if (key !in keys) this else SavedSet(keys - key)

    /** What a press on the plus or tick asks the Engine for: true to save, false to remove. */
    fun pressWouldSave(key: SavedKey): Boolean = key !in keys

    /**
     * The saved key whose id is [id], for a card the phone cannot work out a kind for from the card alone (a curated collection or a
     * playlist saved from the Dashboard). Null when nothing saved has that id.
     */
    fun keyForId(id: String?): SavedKey? {
        val wanted = id?.trim()?.lowercase()?.takeIf(String::isNotEmpty) ?: return null
        return keys.firstOrNull { it.id == wanted }
    }

    companion object {
        val Empty = SavedSet(emptySet())

        fun of(items: List<SavedItem>): SavedSet = SavedSet(items.map { it.key }.toSet())
    }
}

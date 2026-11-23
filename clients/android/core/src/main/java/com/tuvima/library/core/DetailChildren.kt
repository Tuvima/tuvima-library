package com.tuvima.library.core

import org.json.JSONObject

// What a Details page lists under a title: a show's seasons and episodes, an album's tracks, an audiobook's parts, a playlist's
// entries. The Engine already sends them with the page (`mediaGroups`, `MediaGroupingViewModel` in Contracts), so downloading a whole
// season, album or audiobook needs no new connection point. Pure Kotlin: the parse and the planning can be checked on the JVM.

/** One owned file a person may keep offline, as a Details page lists it. [workId] is the Engine's work id (needed for bookmarks). */
data class DetailChild(
    val workId: String?,
    val assetId: String,
    val title: String,
    val subtitle: String?,
    val artworkUrl: String?,
    /** The name the download uses for its kind (`movie`, `tvEpisode`, `musicTrack`, `audiobook`); see [DetailChildren.downloadTypeFor]. */
    val entityType: String,
    val trackNumber: String?,
    val groupKey: String,
    val groupTitle: String,
)

/** One group of a Details page (a season, an album's "Tracks", an audiobook's "Tracks") with the owned files that can be downloaded. */
data class DetailChildGroup(
    val key: String,
    val title: String,
    val children: List<DetailChild>,
    /** Entries the group lists that this library does not hold or that cannot be kept offline (containers, books, comics). */
    val skipped: Int,
)

object DetailChildren {
    /** The group the Engine fills with related, non-member titles; never part of "download everything". */
    const val ALSO_GROUP_KEY = "also-in-this-world"

    /** `DetailEntityType` in Contracts, in order: the Engine sends the number (no string converter is configured). */
    private val wireNames = listOf(
        "Work", "Movie", "MovieSeries", "TvShow", "TvSeason", "TvEpisode", "Book", "BookSeries",
        "Audiobook", "ComicIssue", "ComicSeries", "MusicAlbum", "Person", "Universe", "Collection",
    )

    /** The wire name of an entity type sent as a number or as a word; blank when unknown. */
    fun wireName(value: Any?): String = when (value) {
        is Number -> wireNames.getOrNull(value.toInt()).orEmpty()
        is String -> value.trim().toIntOrNull()?.let { wireNames.getOrNull(it) } ?: value.trim()
        else -> ""
    }

    /**
     * The download kind of an item: a movie, an episode, a song or an audiobook. Null for anything a phone cannot keep offline
     * (a book, a comic, or a container such as a show or Series, which is not a file).
     */
    fun downloadTypeFor(wire: String): String? = when (wire.lowercase()) {
        "movie" -> "movie"
        "tvepisode" -> "tvEpisode"
        "audiobook" -> "audiobook"
        // The Engine types a music track as a plain Work (media type music): that is the only plain Work a Details list carries a file for.
        "work" -> "musicTrack"
        else -> null
    }

    /**
     * Reads the downloadable files of a Details answer. A file is listed once however many chapters point at it (an audiobook lists
     * a row per chapter, all naming the same file). Entries that are not owned, have no file, or are containers or books are counted
     * in [DetailChildGroup.skipped]. The "Also in this world" group never counts.
     */
    fun parse(json: JSONObject, absolute: (String?) -> String?): List<DetailChildGroup> {
        val groups = json.optJSONArray("mediaGroups") ?: return emptyList()
        val seen = HashSet<String>()
        val result = mutableListOf<DetailChildGroup>()
        for (index in 0 until groups.length()) {
            val group = groups.optJSONObject(index) ?: continue
            val key = group.optString("key")
            if (key.equals(ALSO_GROUP_KEY, ignoreCase = true)) continue
            val title = group.optionalString("title") ?: key
            val items = group.optJSONArray("items")
            val children = mutableListOf<DetailChild>()
            var skipped = 0
            if (items != null) {
                for (itemIndex in 0 until items.length()) {
                    val item = items.optJSONObject(itemIndex) ?: continue
                    val assetId = item.optionalString("assetId")
                    val type = downloadTypeFor(wireName(item.opt("entityType")))
                    if (assetId == null || type == null || !item.optBoolean("isOwned", true)) {
                        skipped += 1
                        continue
                    }
                    // A second row for a file already listed (an audiobook's next chapter) is the same download.
                    if (!seen.add(assetId)) continue
                    children.add(
                        DetailChild(
                            workId = item.optionalString("id"),
                            assetId = assetId,
                            title = item.optionalString("title") ?: "Download",
                            subtitle = item.optionalString("subtitle"),
                            artworkUrl = absolute(item.optionalString("artworkUrl")),
                            entityType = type,
                            trackNumber = item.optionalString("trackNumber"),
                            groupKey = key,
                            groupTitle = title,
                        ),
                    )
                }
            }
            if (children.isNotEmpty() || skipped > 0) result.add(DetailChildGroup(key, title, children, skipped))
        }
        return result
    }
}

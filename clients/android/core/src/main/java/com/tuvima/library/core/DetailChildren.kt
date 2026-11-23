package com.tuvima.library.core

import org.json.JSONObject

// What a Details page lists under a title: a show's seasons and episodes, an album's tracks, an audiobook's parts, a playlist's
// entries. The Engine already sends them with the page, so downloading a whole season, album or audiobook needs no new connection
// point. Albums, audiobooks and Series come as `mediaGroups` (`MediaGroupingViewModel` in Contracts); a show or season comes as
// `sequencePlacement.groups` (`SequencePlacementViewModel`), because the Engine sends `mediaGroups: []` for a show. Pure Kotlin: the
// parse and the planning can be checked on the JVM.

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
    /** The show an episode belongs to (its work id), when the page says so; the page's own id otherwise. */
    val showId: String? = null,
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

    /** `DetailEntityType` in Contracts: the Engine sends the number (no string converter is configured), keyed here by that number. */
    private val wireNamesByNumber: Map<Int, String> = mapOf(
        0 to "Work",
        1 to "Movie",
        2 to "MovieSeries",
        3 to "TvShow",
        4 to "TvSeason",
        5 to "TvEpisode",
        6 to "Book",
        7 to "BookSeries",
        8 to "Audiobook",
        9 to "ComicIssue",
        10 to "ComicSeries",
        11 to "MusicAlbum",
        12 to "Person",
        13 to "Universe",
        14 to "Collection",
    )

    /** The wire name of an entity type sent as a number or as a word (any case); blank when unknown. */
    fun wireName(value: Any?): String = when (value) {
        is Number -> wireNamesByNumber[value.toInt()].orEmpty()
        is String -> {
            val text = value.trim()
            val number = text.toIntOrNull()
            if (number != null) {
                wireNamesByNumber[number].orEmpty()
            } else {
                wireNamesByNumber.values.firstOrNull { it.equals(text, ignoreCase = true) } ?: text
            }
        }
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
     * Reads the downloadable files of a Details answer: the `mediaGroups` (album, audiobook, Series) and the episodes of
     * `sequencePlacement` (show, season). A file is listed once however many rows point at it (an audiobook lists a row per
     * chapter, all naming the same file). Entries that are not owned, have no file, or are containers or books are counted
     * in [DetailChildGroup.skipped]. The "Also in this world" group never counts.
     */
    fun parse(json: JSONObject, absolute: (String?) -> String?): List<DetailChildGroup> {
        val seen = HashSet<String>()
        val result = mutableListOf<DetailChildGroup>()
        val groups = json.optJSONArray("mediaGroups")
        if (groups != null) {
            for (index in 0 until groups.length()) {
                val group = groups.optJSONObject(index) ?: continue
                val key = group.optString("key")
                if (key.equals(ALSO_GROUP_KEY, ignoreCase = true)) continue
                val title = group.optionalString("title") ?: key
                val items = group.optJSONArray("items")
                val objects = (0 until (items?.length() ?: 0)).mapNotNull { items?.optJSONObject(it) }
                addGroup(result, seen, key, title, objects) { item -> mediaGroupChild(item, key, title, absolute) }
            }
        }
        val pageId = json.optionalString("id")
        sequenceGroups(json.optJSONObject("sequencePlacement")).forEach { (key, title, objects) ->
            addGroup(result, seen, key, title, objects) { item -> episodeChild(item, key, title, pageId, absolute) }
        }
        return result
    }

    private data class RawGroup(val key: String, val title: String, val items: List<JSONObject>)

    /** The episode groups of a show page: the groups, or (when none carries items) the ordered list split by its own group keys. */
    private fun sequenceGroups(placement: JSONObject?): List<RawGroup> {
        if (placement == null) return emptyList()
        val groups = placement.optJSONArray("groups")
        val fromGroups = mutableListOf<RawGroup>()
        if (groups != null) {
            for (index in 0 until groups.length()) {
                val group = groups.optJSONObject(index) ?: continue
                val key = group.optString("key")
                if (key.equals(ALSO_GROUP_KEY, ignoreCase = true)) continue
                val items = group.optJSONArray("items")
                val objects = (0 until (items?.length() ?: 0)).mapNotNull { items?.optJSONObject(it) }
                fromGroups.add(RawGroup(key, group.optionalString("title") ?: key, objects))
            }
        }
        if (fromGroups.any { it.items.isNotEmpty() }) return fromGroups
        val ordered = placement.optJSONArray("orderedItems") ?: return fromGroups
        val byKey = LinkedHashMap<String, MutableList<JSONObject>>()
        val titles = HashMap<String, String>()
        for (index in 0 until ordered.length()) {
            val item = ordered.optJSONObject(index) ?: continue
            val key = item.optionalString("groupKey") ?: "episodes"
            byKey.getOrPut(key) { mutableListOf() }.add(item)
            titles.putIfAbsent(key, item.optionalString("groupTitle") ?: key)
        }
        return byKey.map { (key, items) -> RawGroup(key, titles[key] ?: key, items) }
    }

    private fun addGroup(
        result: MutableList<DetailChildGroup>,
        seen: MutableSet<String>,
        key: String,
        title: String,
        items: List<JSONObject>,
        read: (JSONObject) -> DetailChild?,
    ) {
        val children = mutableListOf<DetailChild>()
        var skipped = 0
        for (item in items) {
            val child = read(item)
            if (child == null) {
                skipped += 1
                continue
            }
            // A second row for a file already listed (an audiobook's next chapter) is the same download.
            if (seen.add(child.assetId)) children.add(child)
        }
        if (children.isNotEmpty() || skipped > 0) result.add(DetailChildGroup(key, title, children, skipped))
    }

    private fun mediaGroupChild(item: JSONObject, key: String, title: String, absolute: (String?) -> String?): DetailChild? {
        val assetId = item.optionalString("assetId")
        val type = downloadTypeFor(wireName(item.opt("entityType")))
        if (assetId == null || type == null || !item.optBoolean("isOwned", true)) return null
        return DetailChild(
            workId = item.optionalString("id"),
            assetId = assetId,
            title = item.optionalString("title") ?: "Download",
            subtitle = item.optionalString("subtitle"),
            artworkUrl = absolute(item.optionalString("artworkUrl")),
            entityType = type,
            trackNumber = item.optionalString("trackNumber"),
            groupKey = key,
            groupTitle = title,
        )
    }

    /** An owned episode of a show page. Its file is named by `episodeContext.episodeAssetId`; an episode without one is not owned. */
    private fun episodeChild(item: JSONObject, key: String, title: String, pageId: String?, absolute: (String?) -> String?): DetailChild? {
        val type = downloadTypeFor(wireName(item.opt("entityType")))
        if (type != "tvEpisode" || !item.optBoolean("isOwned", false)) return null
        val context = item.optJSONObject("episodeContext") ?: return null
        val assetId = context.optionalString("episodeAssetId") ?: return null
        val season = context.optionalInt("seasonNumber")
        val number = context.optionalInt("episodeNumber")
        return DetailChild(
            workId = context.optionalString("episodeWorkId") ?: item.optionalString("id"),
            assetId = assetId,
            title = context.optionalString("episodeTitle") ?: item.optionalString("title") ?: "Episode",
            subtitle = null,
            artworkUrl = absolute(item.optionalString("episodeStillUrl") ?: item.optionalString("artworkUrl")),
            entityType = type,
            trackNumber = number?.toString(),
            groupKey = season?.let { "season-$it" } ?: item.optionalString("groupKey") ?: key,
            groupTitle = item.optionalString("groupTitle") ?: title,
            showId = context.optionalString("showWorkId") ?: pageId,
        )
    }
}
